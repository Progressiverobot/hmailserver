create table hm_messageindexterms
(
	mitmessageid bigint not null,
	mitaccountid int not null,
	mitterm nvarchar(64) not null
)

CREATE INDEX idx_hm_messageindexterms_lookup ON hm_messageindexterms (mitaccountid, mitterm)

CREATE INDEX idx_hm_messageindexterms_message ON hm_messageindexterms (mitmessageid)

create table hm_messageindexstate
(
	misaccountid int not null,
	mishighwatermark bigint not null
)

CREATE INDEX idx_hm_messageindexstate ON hm_messageindexstate (misaccountid)

update hm_dbversion set value = 6023
