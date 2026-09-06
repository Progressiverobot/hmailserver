create table hm_archiveindex
(
	archiveid int identity(1,1) not null,
	archivetime datetime not null,
	archivedomain nvarchar(255) not null,
	archivemailbox nvarchar(255) not null,
	archivedirection int not null,
	archivesender nvarchar(255) not null,
	archiverecipients nvarchar(1000) not null,
	archivesubject nvarchar(255) not null,
	archivemessageid nvarchar(255) not null,
	archivepath nvarchar(1000) not null,
	archivesize bigint not null,
	archivehold int not null
)

ALTER TABLE hm_archiveindex ADD CONSTRAINT hm_archiveindex_pk PRIMARY KEY (archiveid)

CREATE INDEX idx_hm_archiveindex_domain_time ON hm_archiveindex (archivedomain, archivetime)

CREATE INDEX idx_hm_archiveindex_path ON hm_archiveindex (archivepath)

update hm_dbversion set value = 6029
