create table hm_archiveindex
(
	archiveid int auto_increment not null, primary key(`archiveid`), unique(`archiveid`),
	archivetime datetime not null,
	archivedomain varchar(255) not null,
	archivemailbox varchar(255) not null,
	archivedirection int not null,
	archivesender varchar(255) not null,
	archiverecipients varchar(1000) not null,
	archivesubject varchar(255) not null,
	archivemessageid varchar(255) not null,
	archivepath varchar(1000) not null,
	archivesize bigint not null,
	archivehold int not null
) DEFAULT CHARSET=utf8;

CREATE INDEX idx_hm_archiveindex_domain_time ON hm_archiveindex (archivedomain, archivetime);

CREATE INDEX idx_hm_archiveindex_path ON hm_archiveindex (archivepath(255));

update hm_dbversion set value = 6029;
